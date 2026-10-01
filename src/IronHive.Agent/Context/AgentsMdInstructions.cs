using System.Text;

namespace IronHive.Agent.Context;

/// <summary>Options for <see cref="AgentsMdInstructions"/>.</summary>
public sealed class AgentsMdOptions
{
    /// <summary>
    /// The most characters of AGENTS.md text to add (default 32,000). Past it, the files farthest from the working
    /// directory are left out first — the nearest file wins anyway — and the section says so.
    /// </summary>
    public int MaxCharacters { get; init; } = 32_000;

    /// <summary>The file name to look for (default <c>AGENTS.md</c>).</summary>
    public string FileName { get; init; } = "AGENTS.md";
}

/// <summary>
/// Adds the <c>AGENTS.md</c> files that apply to a working directory to the system instructions: the one in the working
/// directory and those in its parents up to the repository root, root first, so the nearest file comes last and wins
/// where they disagree — the convention of the AGENTS.md standard (agents.md).
/// </summary>
/// <remarks>
/// <para>
/// The walk stops at the repository root — the first directory that holds a <c>.git</c> entry. Files above it (a home
/// directory, a shared parent) are not read. Outside a repository only the working directory's own file is read.
/// </para>
/// <para>
/// The files are read again for every turn, so an edit applies from the next turn on. A directory without the file adds
/// nothing.
/// </para>
/// </remarks>
public sealed class AgentsMdInstructions : ISystemInstructionContributor
{
    private readonly string _workingDirectory;
    private readonly AgentsMdOptions _options;

    /// <summary>Creates the contributor for <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">The directory the agent works in.</param>
    /// <param name="options">Size limit and file name; defaults when <c>null</c>.</param>
    public AgentsMdInstructions(string workingDirectory, AgentsMdOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _options = options ?? new AgentsMdOptions();
        if (_options.MaxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxCharacters, "AgentsMdOptions.MaxCharacters must be positive.");
        }
    }

    /// <inheritdoc />
    public string Name => "agents-md";

    /// <summary>The files that apply, nearest last (those that exist now).</summary>
    public IReadOnlyList<string> FindFiles()
    {
        var directories = new List<string>();
        for (var dir = new DirectoryInfo(_workingDirectory); dir is not null; dir = dir.Parent)
        {
            directories.Add(dir.FullName);
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                break;
            }

            if (dir.Parent is null)
            {
                // No repository above: only the working directory's own file applies.
                directories.RemoveRange(1, directories.Count - 1);
                break;
            }
        }

        directories.Reverse();
        return directories
            .Select(d => Path.Combine(d, _options.FileName))
            .Where(File.Exists)
            .ToList();
    }

    /// <inheritdoc />
    public string? GetInstructions()
    {
        var files = FindFiles();
        if (files.Count == 0)
        {
            return null;
        }

        var root = Path.GetDirectoryName(files[0])!;
        var sections = new List<string>();
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file, Encoding.UTF8).Trim();
            }
            catch (IOException)
            {
                continue;
            }

            if (text.Length == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            sections.Add($"## {relative}\n\n{text}");
        }

        if (sections.Count == 0)
        {
            return null;
        }

        // Over the limit: leave out the farthest files first; the nearest one is what applies anyway.
        var omitted = 0;
        while (sections.Count > 1 && sections.Sum(s => s.Length) > _options.MaxCharacters)
        {
            sections.RemoveAt(0);
            omitted++;
        }

        var builder = new StringBuilder();
        builder.Append("# Project instructions (").Append(_options.FileName).Append(")\n\n");
        builder.Append("These files apply to the working directory, outermost first. Where they disagree, the later (nearer) file wins.");
        if (omitted > 0)
        {
            builder.Append(' ').Append(omitted).Append(" outer file(s) were left out to stay within the size limit.");
        }

        foreach (var section in sections)
        {
            builder.Append("\n\n").Append(section);
        }

        var result = builder.ToString();
        return result.Length <= _options.MaxCharacters ? result : result[.._options.MaxCharacters];
    }
}
