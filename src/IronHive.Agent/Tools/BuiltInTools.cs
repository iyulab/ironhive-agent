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
/// <remarks>
/// A tool that fails throws: the function-invoking client reports the failure to the model (with its message when
/// <c>IncludeDetailedErrors</c> is set), and the library's loop guards count it as a failure — the repeated-error guard
/// ends a request that keeps failing the same way. A refusal by policy (a path outside
/// <see cref="FileToolOptions.AllowedRoots"/>) is not a failure and is returned as text.
/// </remarks>
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
            AIFunctionFactory.Create(tools.EditFile),
            AIFunctionFactory.Create(tools.DeleteFile),
            AIFunctionFactory.Create(tools.MoveFile),
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

    // A command's output is capped at MaxOutputLength per stream, kept as its beginning plus its end: the end is where a
    // build, test run or script prints the error and the summary.
    private const int OutputHeadChars = 20000;
    private const int OutputTailChars = MaxOutputLength - OutputHeadChars;
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
            throw new FileNotFoundException($"File not found: {path}", path);
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxFileSize)
        {
            throw new InvalidOperationException($"File too large ({fileInfo.Length / 1024}KB). Maximum size is {MaxFileSize / 1024}KB.");
        }

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

    /// <summary>
    /// Replaces exact text in an existing file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alternative is <see cref="WriteFile"/> with the whole content: a one-line change to a 1,400-line file then costs
    /// the model the whole file in output tokens (measured: minutes and ~18k tokens), and a model that mis-copies any other
    /// line, or runs out of output, damages the file.
    /// </para>
    /// <para>
    /// <paramref name="oldText"/> must occur exactly once unless <paramref name="replaceAll"/> — an edit of the wrong
    /// occurrence is the failure this contract exists to stop. Line endings in <paramref name="oldText"/> and
    /// <paramref name="newText"/> are taken as the file's own, so a model that sends <c>\n</c> can edit a CRLF file and
    /// the file keeps CRLF. A write: <see cref="FileToolOptions.WriteInterceptor"/> wraps it, and the default tool-call
    /// policy judges it as an edit (<c>edit_file</c>).
    /// </para>
    /// </remarks>
    /// <param name="path">Relative or absolute path to the file to edit.</param>
    /// <param name="oldText">The exact text to replace.</param>
    /// <param name="newText">The text to put in its place.</param>
    /// <param name="replaceAll">Replace every occurrence instead of requiring exactly one.</param>
    [Description("Replace exact text in an existing file. Prefer this over WriteFile for changing part of a file. oldText " +
                 "must match the file exactly (whitespace included) and occur exactly once - include enough surrounding " +
                 "lines to make it unique - unless replaceAll is true.")]
    public async Task<string> EditFile(
        [Description("Path to the file to edit (relative to working directory or absolute)")] string path,
        [Description("The exact text to replace, copied from the file")] string oldText,
        [Description("The text to put in its place")] string newText,
        [Description("If true, replace every occurrence instead of requiring exactly one")] bool replaceAll = false)
    {
        if (!TryResolvePath(path, out var fullPath, out var refusal))
        {
            return refusal;
        }

        if (!File.Exists(fullPath))
        {
            throw Directory.Exists(fullPath)
                ? new InvalidOperationException($"'{path}' is a directory. EditFile edits files only.")
                : new FileNotFoundException($"File not found: {path}. EditFile changes an existing file; use WriteFile to create one.", path);
        }

        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("oldText is empty. Give the exact text to replace, or use WriteFile to write the whole file.", nameof(oldText));
        }

        if (new FileInfo(fullPath).Length > MaxFileSize)
        {
            throw new InvalidOperationException($"File too large ({new FileInfo(fullPath).Length / 1024}KB). Maximum size is {MaxFileSize / 1024}KB.");
        }

        // Keep the file's encoding and byte-order mark: only the replaced text may change.
        var bytes = await File.ReadAllBytesAsync(fullPath);
        var (encoding, preamble) = DetectEncoding(bytes);
        var content = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var find = ToNewline(oldText, newline);
        var replacement = ToNewline(newText ?? string.Empty, newline);

        var matches = CountOccurrences(content, find);
        if (matches == 0)
        {
            var loose = CountOccurrences(Collapse(content), Collapse(find));
            throw new InvalidOperationException(loose > 0
                ? $"oldText was not found in {path} exactly, but text that differs only in whitespace or indentation was. Read the file and copy the text exactly."
                : $"oldText was not found in {path}. Read the file and copy the text exactly.");
        }

        if (matches > 1 && !replaceAll)
        {
            throw new InvalidOperationException($"oldText occurs {matches} times in {path}. Include more surrounding lines to make it unique, or set replaceAll to replace every occurrence.");
        }

        var edited = content.Replace(find, replacement, StringComparison.Ordinal);
        Func<Task> write = () => File.WriteAllTextAsync(fullPath, edited, encoding);

        string? note = null;
        if (_writeInterceptor is null)
        {
            await write();
        }
        else
        {
            note = await _writeInterceptor.InterceptAsync(fullPath, write);
        }

        return matches == 1
            ? $"Successfully edited {path} (1 replacement){note}"
            : $"Successfully edited {path} ({matches} replacements){note}";

        static string ToNewline(string text, string newline) =>
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);

        static string Collapse(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
    }

    // UTF-8 with or without a byte-order mark, or UTF-16 by its mark. Writing back with the returned encoding emits the
    // same preamble the file had (none for UTF-8 without one).
    private static (Encoding Encoding, int Preamble) DetectEncoding(byte[] bytes) => bytes switch
    {
        [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3),
        [0xFF, 0xFE, ..] => (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2),
        [0xFE, 0xFF, ..] => (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2),
        _ => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0),
    };

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Deletes a file.
    /// </summary>
    /// <remarks>
    /// Not a write: <see cref="FileToolOptions.WriteInterceptor"/> wraps writes and does not run here. The default
    /// tool-call policy judges it as a delete (<c>delete_file</c>).
    /// </remarks>
    /// <param name="path">Relative or absolute path to the file to delete.</param>
    [Description("Delete one file, the one the request identifies. Does not delete directories. If the request does not say " +
                 "which file is meant - the name is not at the given path, or several files could match - delete nothing: " +
                 "tell the user which files match and ask which one to delete.")]
    public string DeleteFile(
        [Description("Path to the file to delete (relative to working directory or absolute)")] string path)
    {
        if (!TryResolvePath(path, out var fullPath, out var refusal))
        {
            return refusal;
        }

        if (Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"'{path}' is a directory. DeleteFile deletes files only.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {path}", path);
        }

        File.Delete(fullPath);
        return $"Successfully deleted file: {path}";
    }

    /// <summary>
    /// Moves or renames a file.
    /// </summary>
    /// <remarks>
    /// Not a write: <see cref="FileToolOptions.WriteInterceptor"/> does not run here. The default tool-call policy judges
    /// the source as a delete and the destination as an edit (<c>move_file</c>), and takes the stricter verdict.
    /// </remarks>
    /// <param name="source">Relative or absolute path of the file to move.</param>
    /// <param name="destination">Relative or absolute path the file moves to, including its file name.</param>
    /// <param name="overwrite">Replace a file already at <paramref name="destination"/>.</param>
    [Description("Move or rename one file, the one the request identifies. Refuses to replace an existing file unless " +
                 "overwrite is true. If the request does not say which file is meant, move nothing and ask.")]
    public string MoveFile(
        [Description("Path of the file to move (relative to working directory or absolute)")] string source,
        [Description("New path of the file, including its file name (relative to working directory or absolute)")] string destination,
        [Description("If true, replace a file already at the destination")] bool overwrite = false)
    {
        if (!TryResolvePath(source, out var sourcePath, out var refusal)
            || !TryResolvePath(destination, out var destinationPath, out refusal))
        {
            return refusal;
        }

        if (!File.Exists(sourcePath))
        {
            throw Directory.Exists(sourcePath)
                ? new InvalidOperationException($"'{source}' is a directory. MoveFile moves files only.")
                : new FileNotFoundException($"File not found: {source}", source);
        }

        if (Directory.Exists(destinationPath))
        {
            throw new InvalidOperationException($"'{destination}' is a directory. Give the destination path including the file name.");
        }

        if (File.Exists(destinationPath) && !overwrite)
        {
            throw new InvalidOperationException($"'{destination}' already exists. Pass overwrite=true to replace it.");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(sourcePath, destinationPath, overwrite);
        return $"Successfully moved file: {source} -> {destination}";
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
            throw new DirectoryNotFoundException($"Directory not found: {path ?? "."}");
        }

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
            throw new DirectoryNotFoundException($"Directory not found: {path ?? "."}");
        }

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
            throw new DirectoryNotFoundException($"Directory not found: {path ?? "."}");
        }

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
        var outputCapture = new HeadTailCapture(OutputHeadChars, OutputTailChars);
        var errorCapture = new HeadTailCapture(OutputHeadChars, OutputTailChars);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputCapture.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorCapture.AppendLine(e.Data);
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

            // What the command printed before it was stopped is often why it hung (a prompt, a retry loop).
            var partial = new StringBuilder();
            partial.AppendLine(CultureInfo.InvariantCulture, $"Command timed out after {timeoutMs}ms");
            AppendStream(partial, "Output so far:", outputCapture.ToString());
            AppendStream(partial, "Stderr so far:", errorCapture.ToString());
            throw new TimeoutException(partial.ToString().TrimEnd());
        }

        var result = new StringBuilder();
        result.AppendLine(CultureInfo.InvariantCulture, $"Exit code: {process.ExitCode}");
        AppendStream(result, "Output:", outputCapture.ToString());
        AppendStream(result, "Stderr:", errorCapture.ToString());

        return result.ToString();
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

    private static void AppendStream(StringBuilder result, string label, string captured)
    {
        if (!string.IsNullOrWhiteSpace(captured))
        {
            result.AppendLine(label);
            result.AppendLine(captured);
        }
    }
}
